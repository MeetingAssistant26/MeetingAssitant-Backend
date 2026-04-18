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

public class ListMeetingsTests : IntegrationTestBase
{
    public ListMeetingsTests(MeetingAssistantWebFactory factory) : base(factory)
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

    [Fact]
    public async Task ListMeetings_NoMeetingsExist_ShouldReturnEmptyList()
    {
        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListMeetings_MultipleMeetingsExist_NoFilter_ShouldReturnAllOrderedDescending()
    {
        // Arrange
        await SeedMeetingAsync(TestOrganizationId, scheduledStart: DateTime.UtcNow.AddDays(1));
        await SeedMeetingAsync(TestOrganizationId, scheduledStart: DateTime.UtcNow.AddDays(2));

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items[0].ScheduledStartUtc.Should().BeAfter(result.Items[1].ScheduledStartUtc);
    }

    [Fact]
    public async Task ListMeetings_FilterUpcoming_ShouldReturnFutureMeetingsOrderedAscending()
    {
        // Arrange
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Scheduled, scheduledStart: DateTime.UtcNow.AddDays(2));
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Scheduled, scheduledStart: DateTime.UtcNow.AddDays(1));
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Completed, scheduledStart: DateTime.UtcNow.AddDays(-1));

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?filter=upcoming&page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items[0].ScheduledStartUtc.Should().BeBefore(result.Items[1].ScheduledStartUtc);
    }

    [Fact]
    public async Task ListMeetings_FilterPast_ShouldReturnPastMeetingsOrderedDescending()
    {
        // Arrange
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Completed, scheduledStart: DateTime.UtcNow.AddDays(-1));
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Cancelled, scheduledStart: DateTime.UtcNow.AddDays(-2));
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Scheduled, scheduledStart: DateTime.UtcNow.AddDays(1));

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?filter=past&page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items[0].ScheduledStartUtc.Should().BeAfter(result.Items[1].ScheduledStartUtc);
    }

    [Fact]
    public async Task ListMeetings_FilterUpcoming_ShouldExcludeCancelledFutureMeetings()
    {
        // Arrange
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Scheduled, scheduledStart: DateTime.UtcNow.AddDays(1));
        await SeedMeetingAsync(TestOrganizationId, status: MeetingStatus.Cancelled, scheduledStart: DateTime.UtcNow.AddDays(2));

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?filter=upcoming&page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle(m => m.Status == MeetingStatus.Scheduled);
    }

    [Fact]
    public async Task ListMeetings_UnknownFilterValue_ShouldActAsNoFilter()
    {
        // Arrange
        await SeedMeetingAsync(TestOrganizationId, scheduledStart: DateTime.UtcNow.AddDays(1));
        await SeedMeetingAsync(TestOrganizationId, scheduledStart: DateTime.UtcNow.AddDays(2));

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?filter=unknown&page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task ListMeetings_PaginationPage1PageSize2_ShouldReturn2Items()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            await SeedMeetingAsync(TestOrganizationId);
        }

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=1&pageSize=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(5);
        result.Items.Should().HaveCount(2);
        result.Page.Should().Be(1);
    }

    [Fact]
    public async Task ListMeetings_PaginationPage3PageSize2_ShouldReturn1Item()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            await SeedMeetingAsync(TestOrganizationId);
        }

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=3&pageSize=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(5);
        result.Items.Should().HaveCount(1);
        result.Page.Should().Be(3);
    }

    [Fact]
    public async Task ListMeetings_PageSizeGreaterThan100_ShouldClampTo100()
    {
        // Arrange
        await SeedMeetingAsync(TestOrganizationId);

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=1&pageSize=150");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task ListMeetings_PageLessThan1_ShouldClampTo1()
    {
        // Arrange
        await SeedMeetingAsync(TestOrganizationId);

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=-5&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.Page.Should().Be(1);
    }

    [Fact]
    public async Task ListMeetings_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var unauthClient = Factory.CreateClient();

        // Act
        var response = await unauthClient.GetAsync($"/api/organizations/{TestOrganizationId}/meetings");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListMeetings_RouteOrgIdDiffersFromJwtOrgId_ShouldReturnForbidden()
    {
        // Arrange
        var otherOrgId = Guid.NewGuid();

        // Act
        var response = await Client.GetAsync($"/api/organizations/{otherOrgId}/meetings");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListMeetings_ShouldIsolateTenants_ReturnOnlyCallersOrgMeetings()
    {
        // Arrange
        var otherOrgId = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Organizations.Add(new Organization { Id = otherOrgId, Name = "Other Org" });
            await db.SaveChangesAsync();
        }

        await SeedMeetingAsync(TestOrganizationId, title: "Caller Org Meeting");
        await SeedMeetingAsync(otherOrgId, title: "Other Org Meeting");

        // Act
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=1&pageSize=20");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MeetingListResponse>();
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle(m => m.Title == "Caller Org Meeting");
    }
}