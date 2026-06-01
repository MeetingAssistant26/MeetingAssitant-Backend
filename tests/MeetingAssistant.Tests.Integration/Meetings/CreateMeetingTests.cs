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

public class CreateMeetingTests : IntegrationTestBase
{
    public CreateMeetingTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private static CreateMeetingRequest CreateValidRequest(
        string title = "Test Meeting",
        string? description = "A test meeting",
        DateTime? start = null,
        DateTime? end = null,
        List<Guid>? tagIds = null)
    {
        var s = start ?? DateTime.UtcNow.AddDays(1);
        var e = end ?? s.AddHours(1);
        return new CreateMeetingRequest(title, description, s, e, tagIds);
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

    [Fact]
    public async Task CreateMeeting_ValidAdminRequest_ShouldReturnOk_AndCreateMeeting()
    {
        // Arrange
        var request = CreateValidRequest();

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.Title.Should().Be(request.Title);
        result.Description.Should().Be(request.Description);
        result.Status.Should().Be(MeetingStatus.Scheduled);
        result.AiAssistantEnabled.Should().BeTrue();
        result.ScheduledStartUtc.Should().BeCloseTo(request.ScheduledStartUtc, TimeSpan.FromSeconds(1));
        result.ScheduledEndUtc.Should().BeCloseTo(request.ScheduledEndUtc, TimeSpan.FromSeconds(1));
        
        result.Participants.Should().ContainSingle(p => p.UserId == TestUserId);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbMeeting = await db.Meetings.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.Id == result.Id);
        dbMeeting.Should().NotBeNull();
        dbMeeting!.AiAssistantEnabled.Should().BeTrue();
        var dbParticipant = await db.MeetingParticipants.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.MeetingId == result.Id && p.UserId == TestUserId);
        dbParticipant.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateMeeting_ValidMemberRequest_ShouldReturnOk()
    {
        // Arrange
        var userId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(userId, TestOrganizationId, isEnabled: true, orgRole: OrganizationRole.Member);
        var client = CreateAuthenticatedClient(userId, TestOrganizationId);
        var request = CreateValidRequest(title: "Member Created Meeting");

        // Act
        var response = await client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.Title.Should().Be(request.Title);
        result.Participants.Should().ContainSingle(p => p.UserId == userId);
    }

    [Fact]
    public async Task CreateMeeting_NullDescription_ShouldReturnOk()
    {
        // Arrange
        var request = CreateValidRequest(title: "No Desc", description: null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.Description.Should().BeNull();
    }

    [Fact]
    public async Task CreateMeeting_NullTagIds_ShouldReturnOk()
    {
        // Arrange
        var request = CreateValidRequest(title: "No Tags", tagIds: null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        result.Should().NotBeNull();
        result!.TagIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateMeeting_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var unauthClient = Factory.CreateClient();
        var request = CreateValidRequest();

        // Act
        var response = await unauthClient.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateMeeting_RouteOrgIdDiffersFromJwtOrgId_ShouldReturnForbidden()
    {
        // Arrange
        var otherOrgId = Guid.NewGuid();
        var request = CreateValidRequest();

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{otherOrgId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateMeeting_UserNotMemberOfOrg_ShouldReturnForbidden()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = new ApplicationUser { Id = userId, Email = "not-member@test.com", UserName = "not-member@test.com" };
            db.Users.Add(user);
            db.Organizations.Add(new Organization { Id = orgId, Name = "Another Org" });
            await db.SaveChangesAsync();
        }
        
        var client = CreateAuthenticatedClient(userId, orgId);
        var request = CreateValidRequest();

        // Act
        var response = await client.PostAsJsonAsync($"/api/organizations/{orgId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateMeeting_UserMembershipIsDisabled_ShouldReturnForbidden()
    {
        // Arrange
        var userId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(userId, TestOrganizationId, isEnabled: false);
        var client = CreateAuthenticatedClient(userId, TestOrganizationId);
        var request = CreateValidRequest();

        // Act
        var response = await client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateMeeting_UserIsGuest_ShouldReturnForbidden()
    {
        // Arrange
        var userId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(userId, TestOrganizationId, isEnabled: true, orgRole: OrganizationRole.Guest);
        var client = CreateAuthenticatedClient(userId, TestOrganizationId);
        var request = CreateValidRequest();

        // Act
        var response = await client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Guests are not allowed to create or modify meetings.");
    }

    [Fact]
    public async Task CreateMeeting_EmptyTitle_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(title: string.Empty);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task CreateMeeting_TitleExceedsLength_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(title: new string('a', 201));

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task CreateMeeting_DescriptionExceedsLength_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(description: new string('a', 2001));

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Description");
    }

    [Fact]
    public async Task CreateMeeting_ScheduledStartUtcInPast_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(start: DateTime.UtcNow.AddMinutes(-5));

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ScheduledStartUtc");
    }

    [Fact]
    public async Task CreateMeeting_ScheduledEndUtcBeforeStart_ShouldReturnBadRequest()
    {
        // Arrange
        var start = DateTime.UtcNow.AddDays(1);
        var end = start.AddMinutes(-30);
        var request = CreateValidRequest(start: start, end: end);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ScheduledEndUtc");
    }

    [Fact]
    public async Task CreateMeeting_MissingScheduledStartUtc_ShouldReturnBadRequest()
    {
        // Arrange
        var request = new CreateMeetingRequest("Title", "Desc", default, DateTime.UtcNow.AddDays(1), null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ScheduledStartUtc");
    }

    [Fact]
    public async Task CreateMeeting_TagIdNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var request = CreateValidRequest(tagIds: new List<Guid> { Guid.NewGuid() });

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("One or more specified tags were not found or are inactive.");
    }
}
