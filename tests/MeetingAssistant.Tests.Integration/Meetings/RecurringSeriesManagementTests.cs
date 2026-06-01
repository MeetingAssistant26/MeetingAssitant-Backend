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

public class RecurringSeriesManagementTests : IntegrationTestBase
{
    public RecurringSeriesManagementTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private static TimeSpan FutureTimeOfDay()
    {
        var candidate = DateTime.UtcNow.TimeOfDay.Add(TimeSpan.FromHours(2));
        return candidate >= TimeSpan.FromDays(1) ? TimeSpan.FromHours(1) : new TimeSpan(candidate.Hours, candidate.Minutes, 0);
    }

    private static CreateRecurringMeetingRequest CreateRequest(string title = "Series Management")
    {
        var start = FutureTimeOfDay();
        return new CreateRecurringMeetingRequest(
            title,
            "Managed recurring meeting",
            start,
            start.Add(TimeSpan.FromMinutes(30)),
            new RecurrenceConfigDto(
                RecurrenceFrequency.Daily,
                1,
                null,
                DateTime.UtcNow.Date.AddDays(4)),
            null);
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateToken(userId, orgId));
        return client;
    }

    private async Task<ApplicationUser> SeedUserAndMembershipAsync(
        Guid userId,
        Guid orgId,
        OrganizationRole orgRole = OrganizationRole.Member,
        bool enabled = true)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = new ApplicationUser
        {
            Id = userId,
            Email = $"series-{userId:N}@test.com",
            UserName = $"series-{userId:N}@test.com",
            NormalizedEmail = $"SERIES-{userId:N}@TEST.COM",
            NormalizedUserName = $"SERIES-{userId:N}@TEST.COM",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        };

        if (!await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId))
            db.Users.Add(user);

        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId))
            db.Organizations.Add(new Organization { Id = orgId, Name = $"Org {orgId:N}", Slug = $"org-{orgId:N}"[..20] });

        if (!await db.UserOrgMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == userId && m.OrganizationId == orgId))
        {
            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = userId,
                OrganizationId = orgId,
                OrgRole = orgRole,
                IsEnabled = enabled
            });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<RecurringMeetingCreationResponse> CreateSeriesAsync(string title = "Series Management")
    {
        var response = await Client.PostAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring", CreateRequest(title));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<RecurringMeetingCreationResponse>();
        result.Should().NotBeNull();
        result!.SeriesId.Should().NotBeNull();
        result.Meetings.Should().NotBeEmpty();
        return result;
    }

    [Fact]
    public async Task ListAndGetRecurringSeries_ShouldReturnSeriesIdentityAndOccurrences()
    {
        var created = await CreateSeriesAsync("List Detail Series");

        var listResponse = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring?page=1&pageSize=10");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResponse.Content.ReadFromJsonAsync<RecurringSeriesListResponse>();
        list.Should().NotBeNull();
        list!.Items.Should().ContainSingle(s => s.Id == created.SeriesId);
        list.Items.Single(s => s.Id == created.SeriesId).OccurrenceCount.Should().Be(created.Count);

        var detailResponse = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring/{created.SeriesId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<RecurringSeriesResponse>();
        detail.Should().NotBeNull();
        detail!.Id.Should().Be(created.SeriesId!.Value);
        detail.Title.Should().Be("List Detail Series");
        detail.Occurrences.Should().HaveCount(created.Count);
        detail.UpdateSemantics.Should().Contain("Future linked scheduled occurrences only");
    }

    [Fact]
    public async Task UpdateRecurringSeries_TitleOnly_ShouldRewriteFutureScheduledOccurrencesOnly()
    {
        var created = await CreateSeriesAsync("Before Update");
        var completedOccurrenceId = created.Meetings.First().Id;

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var occurrence = await db.Meetings.IgnoreQueryFilters().SingleAsync(m => m.Id == completedOccurrenceId);
            occurrence.Status = MeetingStatus.Completed;
            occurrence.ScheduledStartUtc = DateTime.UtcNow.AddDays(-1);
            occurrence.ScheduledEndUtc = DateTime.UtcNow.AddDays(-1).AddMinutes(30);
            await db.SaveChangesAsync();
        }

        var update = new UpdateRecurringSeriesRequest("After Update", "Updated description", null, null, null, null);
        var response = await Client.PatchAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring/{created.SeriesId}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RecurringSeriesResponse>();
        body.Should().NotBeNull();
        body!.Title.Should().Be("After Update");

        await using var verifyScope = Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var occurrences = await verifyDb.Meetings.IgnoreQueryFilters()
            .Where(m => m.RecurringSeriesId == created.SeriesId)
            .ToListAsync();

        occurrences.Single(m => m.Id == completedOccurrenceId).Title.Should().Be("Before Update");
        occurrences.Where(m => m.Id != completedOccurrenceId && m.Status == MeetingStatus.Scheduled)
            .Should().OnlyContain(m => m.Title == "After Update" && m.Description == "Updated description");
    }

    [Fact]
    public async Task UpdateRecurringSeries_PatternChange_ShouldCancelOldFutureAndGenerateNewOccurrences()
    {
        var created = await CreateSeriesAsync("Pattern Before");
        var originalFutureIds = created.Meetings.Select(m => m.Id).ToHashSet();
        var start = FutureTimeOfDay().Add(TimeSpan.FromHours(1));
        if (start >= TimeSpan.FromDays(1))
            start = TimeSpan.FromHours(2);

        var update = new UpdateRecurringSeriesRequest(
            "Pattern After",
            "Pattern changed",
            start,
            start.Add(TimeSpan.FromMinutes(45)),
            new RecurrenceConfigDto(RecurrenceFrequency.Weekly, 1, new[] { DateTime.UtcNow.DayOfWeek }, DateTime.UtcNow.Date.AddDays(14)),
            null);

        var response = await Client.PutAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring/{created.SeriesId}", update);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RecurringSeriesResponse>();
        body.Should().NotBeNull();
        body!.Title.Should().Be("Pattern After");
        body.Occurrences.Should().Contain(o => !originalFutureIds.Contains(o.Id));
        body.Occurrences.Where(o => originalFutureIds.Contains(o.Id)).Should().OnlyContain(o => o.Status == MeetingStatus.Cancelled);
        body.Occurrences.Where(o => !originalFutureIds.Contains(o.Id)).Should().OnlyContain(o => o.Status == MeetingStatus.Scheduled && o.Title == "Pattern After");
    }

    [Fact]
    public async Task DeleteRecurringSeries_ShouldCancelSeriesAndFutureScheduledOccurrencesOnly()
    {
        var created = await CreateSeriesAsync("Delete Series");
        var completedOccurrenceId = created.Meetings.First().Id;

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var occurrence = await db.Meetings.IgnoreQueryFilters().SingleAsync(m => m.Id == completedOccurrenceId);
            occurrence.Status = MeetingStatus.Completed;
            occurrence.ScheduledStartUtc = DateTime.UtcNow.AddDays(-1);
            occurrence.ScheduledEndUtc = DateTime.UtcNow.AddDays(-1).AddMinutes(30);
            await db.SaveChangesAsync();
        }

        var response = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring/{created.SeriesId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RecurringSeriesResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be(RecurringMeetingSeriesStatus.Cancelled);
        body.CancelledAtUtc.Should().NotBeNull();
        body.Occurrences.Single(o => o.Id == completedOccurrenceId).Status.Should().Be(MeetingStatus.Completed);
        body.Occurrences.Where(o => o.Id != completedOccurrenceId).Should().OnlyContain(o => o.Status == MeetingStatus.Cancelled);

        var secondDelete = await Client.DeleteAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring/{created.SeriesId}");
        secondDelete.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RecurringSeriesManagement_ShouldEnforceAuthAndOrgIsolation()
    {
        var created = await CreateSeriesAsync("Isolation Series");

        var unauthenticated = Factory.CreateClient();
        var unauthenticatedResponse = await unauthenticated.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring");
        unauthenticatedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var wrongRouteOrgResponse = await Client.GetAsync($"/api/organizations/{Guid.NewGuid()}/meetings/recurring/{created.SeriesId}");
        wrongRouteOrgResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var memberId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(memberId, TestOrganizationId, OrganizationRole.Member);
        var memberClient = CreateAuthenticatedClient(memberId, TestOrganizationId);
        var forbiddenUpdate = await memberClient.PatchAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/meetings/recurring/{created.SeriesId}",
            new UpdateRecurringSeriesRequest("Nope", null, null, null, null, null));
        forbiddenUpdate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateRecurringSeries_InvalidRecurrence_ShouldReturnBadRequest()
    {
        var created = await CreateSeriesAsync("Invalid Update Series");

        var update = new UpdateRecurringSeriesRequest(
            null,
            null,
            null,
            null,
            new RecurrenceConfigDto(RecurrenceFrequency.Weekly, 1, null, DateTime.UtcNow.Date.AddDays(14)),
            null);

        var response = await Client.PatchAsJsonAsync($"/api/organizations/{TestOrganizationId}/meetings/recurring/{created.SeriesId}", update);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Errors.Should().ContainKey("Recurrence.DaysOfWeek");
    }
}
