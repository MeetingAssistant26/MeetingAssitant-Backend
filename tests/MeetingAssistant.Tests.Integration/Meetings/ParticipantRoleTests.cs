using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
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

public class ParticipantRoleTests : IntegrationTestBase
{
    public ParticipantRoleTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task UpdateParticipantRole_HostUpdatesParticipant_ReturnsUpdatedParticipant()
    {
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.CoHost));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ParticipantResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be(targetUserId);
        body.Role.Should().Be(MeetingRole.CoHost);

        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.CoHost);
    }

    [Fact]
    public async Task UpdateParticipantRole_HostDemotesCoHost_ReturnsUpdatedParticipant()
    {
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.CoHost);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Observer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.Observer);
    }

    [Fact]
    public async Task UpdateParticipantRole_CoHostUpdatesParticipantToObserver_ReturnsUpdatedParticipant()
    {
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.CoHost);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Observer));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.Observer);
    }

    [Fact]
    public async Task UpdateParticipantRole_CoHostCannotPromoteParticipantToCoHost_ReturnsForbidden()
    {
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.CoHost);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.CoHost));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.Participant);
    }

    [Fact]
    public async Task UpdateParticipantRole_CoHostCannotUpdateHost_ReturnsForbidden()
    {
        var hostId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.CoHost);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, hostId),
            new UpdateParticipantRoleRequest(MeetingRole.Participant));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var role = await GetParticipantRoleAsync(meeting.Id, hostId);
        role.Should().Be(MeetingRole.Host);
    }

    [Fact]
    public async Task UpdateParticipantRole_ParticipantCaller_ReturnsForbidden()
    {
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(hostId, TestOrganizationId);
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, hostId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Participant);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Observer);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Participant));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.Observer);
    }

    [Fact]
    public async Task UpdateParticipantRole_DemotingLastHost_ReturnsForbidden()
    {
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, TestUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Participant));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var role = await GetParticipantRoleAsync(meeting.Id, TestUserId);
        role.Should().Be(MeetingRole.Host);
    }

    [Fact]
    public async Task UpdateParticipantRole_MissingMeetingRole_ReturnsBadRequestAndDoesNotChangeRole()
    {
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await Client.PutAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.Participant);
    }

    [Fact]
    public async Task UpdateParticipantRole_TargetNotParticipant_ReturnsNotFound()
    {
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Participant));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateParticipantRole_TargetParticipantWithoutActiveOrgMembership_ReturnsForbidden()
    {
        var targetUserId = Guid.NewGuid();
        await SeedUserAsync(targetUserId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Observer));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.Participant);
    }

    [Fact]
    public async Task UpdateParticipantRole_CallerNotMeetingParticipant_ReturnsForbidden()
    {
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await Client.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Observer));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateParticipantRole_CallerWithoutOrgMembership_ReturnsForbidden()
    {
        var callerId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        await SeedUserAsync(callerId);
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var callerClient = CreateAuthenticatedClient(callerId, TestOrganizationId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, callerId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await callerClient.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Observer));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateParticipantRole_CrossOrgRouteMismatch_ReturnsForbidden()
    {
        var otherOrgId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(callerId, otherOrgId);
        await SeedUserAndMembershipAsync(targetUserId, TestOrganizationId);
        var otherOrgClient = CreateAuthenticatedClient(callerId, otherOrgId);
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, targetUserId, MeetingRole.Participant);

        var response = await otherOrgClient.PutAsJsonAsync(
            RoleUrl(TestOrganizationId, meeting.Id, targetUserId),
            new UpdateParticipantRoleRequest(MeetingRole.Observer));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var role = await GetParticipantRoleAsync(meeting.Id, targetUserId);
        role.Should().Be(MeetingRole.Participant);
    }

    private static string RoleUrl(Guid orgId, Guid meetingId, Guid userId) =>
        $"/api/organizations/{orgId}/meetings/{meetingId}/participants/{userId}/role";

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ApplicationUser> SeedUserAndMembershipAsync(
        Guid userId,
        Guid orgId,
        bool isEnabled = true,
        OrganizationRole orgRole = OrganizationRole.Member)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await EnsureUserAsync(db, userId);

        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId))
        {
            db.Organizations.Add(new Organization
            {
                Id = orgId,
                Name = $"Test Org {orgId:N}",
                Slug = $"test-org-{orgId:N}"
            });
        }

        if (!await db.UserOrgMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == userId && m.OrganizationId == orgId))
        {
            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OrganizationId = orgId,
                IsEnabled = isEnabled,
                OrgRole = orgRole
            });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<ApplicationUser> SeedUserAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await EnsureUserAsync(db, userId);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<ApplicationUser> EnsureUserAsync(ApplicationDbContext db, Guid userId)
    {
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (existing != null)
            return existing;

        var email = $"test-{userId:N}@test.com";
        var user = new ApplicationUser
        {
            Id = userId,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            DisplayName = $"Test {userId:N}",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        db.Users.Add(user);
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
            Title = "Participant Role Test Meeting",
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

    private async Task<MeetingRole?> GetParticipantRoleAsync(Guid meetingId, Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.MeetingParticipants
            .IgnoreQueryFilters()
            .Where(p => p.MeetingId == meetingId && p.UserId == userId)
            .Select(p => (MeetingRole?)p.MeetingRole)
            .FirstOrDefaultAsync();
    }
}
