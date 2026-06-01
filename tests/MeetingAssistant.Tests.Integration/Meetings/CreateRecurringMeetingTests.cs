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

public class CreateRecurringMeetingTests : IntegrationTestBase
{
    public CreateRecurringMeetingTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private static CreateRecurringMeetingRequest CreateValidRequest(
        string title = "Recurring Integration Meeting",
        string? description = "Recurring meeting description",
        TimeSpan? scheduledStartTimeUtc = null,
        TimeSpan? scheduledEndTimeUtc = null,
        RecurrenceConfigDto? recurrence = null,
        IReadOnlyList<Guid>? tagIds = null)
    {
        var start = scheduledStartTimeUtc ?? TimeSpan.FromHours(9);
        var end = scheduledEndTimeUtc ?? TimeSpan.FromHours(10);
        var recurrenceConfig = recurrence ?? new RecurrenceConfigDto(
            RecurrenceFrequency.Daily,
            1,
            null,
            DateTime.UtcNow.Date.AddDays(7));

        return new CreateRecurringMeetingRequest(title, description, start, end, recurrenceConfig, tagIds);
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

    private async Task<MeetingTag> SeedMeetingTagAsync(Guid orgId, string name = "Recurring Tag", bool isActive = true)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var orgExists = await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId);
        if (!orgExists)
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = "Test Org" });
            await db.SaveChangesAsync();
        }

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

    [Fact]
    public async Task CreateRecurringMeeting_ValidAdminRequest_ShouldReturnOkAndCreateMeetings()
    {
        // Arrange
        var request = CreateValidRequest(title: "Recurring Admin Success");

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<RecurringMeetingCreationResponse>();
        result.Should().NotBeNull();
        result!.SeriesId.Should().NotBeNull();
        result.Count.Should().BeGreaterThan(0);
        result.Meetings.Should().HaveCount(result.Count);
        result.Meetings.Should().OnlyContain(m => m.Status == MeetingStatus.Scheduled);
        result.Meetings.Should().OnlyContain(m => m.Title == request.Title);
        result.Meetings.Should().OnlyContain(m => m.Participants.Any(p => p.UserId == TestUserId && p.Role == MeetingRole.Host));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var createdMeetingIds = result.Meetings.Select(m => m.Id).ToList();
        var dbMeetings = await db.Meetings.IgnoreQueryFilters().Where(m => createdMeetingIds.Contains(m.Id)).ToListAsync();
        dbMeetings.Should().HaveCount(result.Count);
        dbMeetings.Should().OnlyContain(m => m.Status == MeetingStatus.Scheduled);
        dbMeetings.Should().OnlyContain(m => m.RecurringSeriesId == result.SeriesId);
        dbMeetings.Select(m => m.RecurringOccurrenceIndex).Should().OnlyContain(i => i.HasValue);

        var series = await db.RecurringMeetingSeries.IgnoreQueryFilters().SingleAsync(s => s.Id == result.SeriesId);
        series.Title.Should().Be(request.Title);
        series.CreatedByUserId.Should().Be(TestUserId);

        var hostParticipantsCount = await db.MeetingParticipants.IgnoreQueryFilters()
            .CountAsync(p => createdMeetingIds.Contains(p.MeetingId) && p.UserId == TestUserId && p.MeetingRole == MeetingRole.Host);
        hostParticipantsCount.Should().Be(result.Count);
    }

    [Fact]
    public async Task CreateRecurringMeeting_ValidMemberRequest_ShouldReturnOk()
    {
        // Arrange
        var memberId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(memberId, TestOrganizationId, isEnabled: true, orgRole: OrganizationRole.Member);
        var memberClient = CreateAuthenticatedClient(memberId, TestOrganizationId);

        var request = CreateValidRequest(title: "Recurring Member Success");

        // Act
        var response = await memberClient.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<RecurringMeetingCreationResponse>();
        result.Should().NotBeNull();
        result!.Count.Should().BeGreaterThan(0);
        result.Meetings.Should().OnlyContain(m => m.Participants.Any(p => p.UserId == memberId && p.Role == MeetingRole.Host));
    }

    [Fact]
    public async Task CreateRecurringMeeting_NullTagIds_ShouldReturnOkWithEmptyTagIds()
    {
        // Arrange
        var request = CreateValidRequest(title: "Recurring No Tags", tagIds: null);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<RecurringMeetingCreationResponse>();
        result.Should().NotBeNull();
        result!.Meetings.Should().NotBeEmpty();
        result.Meetings.Should().OnlyContain(m => m.TagIds.Count == 0);
    }

    [Fact]
    public async Task CreateRecurringMeeting_ValidTagIds_ShouldReturnOkWithTagsOnAllMeetings()
    {
        // Arrange
        var tag = await SeedMeetingTagAsync(TestOrganizationId, "Team Sync");
        var request = CreateValidRequest(
            title: "Recurring Tagged",
            tagIds: new List<Guid> { tag.Id });

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<RecurringMeetingCreationResponse>();
        result.Should().NotBeNull();
        result!.Meetings.Should().NotBeEmpty();
        result.Meetings.Should().OnlyContain(m => m.TagIds.Contains(tag.Id));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var meetingIds = result.Meetings.Select(m => m.Id).ToList();
        var linksCount = await db.MeetingMeetingTags.IgnoreQueryFilters().CountAsync(x => meetingIds.Contains(x.MeetingId) && x.MeetingTagId == tag.Id);
        linksCount.Should().Be(result.Count);
    }

    [Fact]
    public async Task CreateRecurringMeeting_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var unauthenticatedClient = Factory.CreateClient();
        var request = CreateValidRequest();

        // Act
        var response = await unauthenticatedClient.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateRecurringMeeting_RouteOrgIdDiffersFromJwtOrgId_ShouldReturnForbidden()
    {
        // Arrange
        var otherOrgId = Guid.NewGuid();
        var request = CreateValidRequest();

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{otherOrgId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateRecurringMeeting_UserNotMemberOfOrg_ShouldReturnForbidden()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var orgId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                Email = $"user-{userId}@test.com",
                UserName = $"user-{userId}@test.com"
            });
            db.Organizations.Add(new Organization { Id = orgId, Name = "Other Org" });
            await db.SaveChangesAsync();
        }

        var client = CreateAuthenticatedClient(userId, orgId);
        var request = CreateValidRequest();

        // Act
        var response = await client.PostAsJsonAsync($"/api/organizations/{orgId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateRecurringMeeting_UserMembershipDisabled_ShouldReturnForbidden()
    {
        // Arrange
        var userId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(userId, TestOrganizationId, isEnabled: false, orgRole: OrganizationRole.Member);
        var disabledClient = CreateAuthenticatedClient(userId, TestOrganizationId);
        var request = CreateValidRequest();

        // Act
        var response = await disabledClient.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateRecurringMeeting_GuestRole_ShouldReturnForbidden()
    {
        // Arrange
        var guestId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(guestId, TestOrganizationId, isEnabled: true, orgRole: OrganizationRole.Guest);
        var guestClient = CreateAuthenticatedClient(guestId, TestOrganizationId);
        var request = CreateValidRequest();

        // Act
        var response = await guestClient.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.Forbidden);
        error.Title.Should().Be("Guests are not allowed to create or modify meetings.");
    }

    [Fact]
    public async Task CreateRecurringMeeting_EmptyTitle_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(title: string.Empty);

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Title");
    }

    [Fact]
    public async Task CreateRecurringMeeting_EndTimeBeforeStart_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(
            scheduledStartTimeUtc: TimeSpan.FromHours(11),
            scheduledEndTimeUtc: TimeSpan.FromHours(10));

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ScheduledEndTimeUtc");
    }

    [Fact]
    public async Task CreateRecurringMeeting_WeeklyWithoutDaysOfWeek_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(
            recurrence: new RecurrenceConfigDto(
                RecurrenceFrequency.Weekly,
                1,
                null,
                DateTime.UtcNow.Date.AddDays(14)));

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Recurrence.DaysOfWeek");
    }

    [Fact]
    public async Task CreateRecurringMeeting_IntervalOutOfRange_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(
            recurrence: new RecurrenceConfigDto(
                RecurrenceFrequency.Daily,
                0,
                null,
                DateTime.UtcNow.Date.AddDays(14)));

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Recurrence.Interval");
    }

    [Fact]
    public async Task CreateRecurringMeeting_EndsAtInPast_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(
            recurrence: new RecurrenceConfigDto(
                RecurrenceFrequency.Daily,
                1,
                null,
                DateTime.UtcNow.AddHours(-1)));

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Recurrence.EndsAtUtc.Value");
    }

    [Fact]
    public async Task CreateRecurringMeeting_TagIdsContainUnknownTag_ShouldReturnNotFound()
    {
        // Arrange
        var request = CreateValidRequest(tagIds: new List<Guid> { Guid.NewGuid() });

        // Act
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.NotFound);
        error.Title.Should().Be("One or more specified tags were not found or are inactive.");
    }
}
