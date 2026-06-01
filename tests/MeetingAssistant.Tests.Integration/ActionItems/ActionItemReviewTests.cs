using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems;

public class ActionItemReviewTests : IntegrationTestBase
{
    public ActionItemReviewTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task ListMeetingActionItems_ShouldExposeRowVersionEtag()
    {
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedActionItemAsync(TestOrganizationId, meeting.Id, TestUserId, "Visible item");

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}/action-items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ActionItemListResponse>();
        body!.Items.Should().ContainSingle();
        body.Items[0].RowVersionEtag.Should().NotBeNullOrWhiteSpace();
        body.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task ApproveActionItem_WithoutIfMatch_ShouldReturnPreconditionRequired()
    {
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        var item = await SeedActionItemAsync(TestOrganizationId, meeting.Id, TestUserId, "Needs precondition");

        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}/action-items/{item.Id}/approve");
        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be((HttpStatusCode)428);
    }

    [Fact]
    public async Task ApproveActionItem_WithStaleIfMatch_ShouldReturnConflict()
    {
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        var item = await SeedActionItemAsync(TestOrganizationId, meeting.Id, TestUserId, "Stale item");

        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}/action-items/{item.Id}/approve");
        request.Headers.TryAddWithoutValidation("If-Match", "stale-etag");

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ApproveActionItem_WithFreshIfMatch_ShouldReturnUpdatedActionItemWithNewEtag()
    {
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        var item = await SeedActionItemAsync(TestOrganizationId, meeting.Id, TestUserId, "Approve item");
        var originalEtag = await GetCurrentEtagAsync(meeting.Id, item.Id);

        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}/action-items/{item.Id}/approve");
        request.Headers.TryAddWithoutValidation("If-Match", originalEtag);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ActionItemResponse>();
        body!.Status.Should().Be(ActionItemStatus.Approved.ToString());
        body.RowVersionEtag.Should().NotBeNullOrWhiteSpace();
        body.RowVersionEtag.Should().NotBe(originalEtag);
    }

    [Fact]
    public async Task SyncActionItem_WithoutIfMatch_ShouldReturnPreconditionRequiredBeforeProviderValidation()
    {
        var meeting = await SeedMeetingAsync(TestOrganizationId);
        await SeedMeetingParticipantAsync(meeting.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        var item = await SeedActionItemAsync(TestOrganizationId, meeting.Id, TestUserId, "Sync item", status: ActionItemStatus.Approved);

        var response = await Client.PostAsync(
            $"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}/action-items/{item.Id}/sync",
            content: null);

        response.StatusCode.Should().Be((HttpStatusCode)428);
    }

    [Fact]
    public async Task ListOrganizationActionItems_ShouldFilterPaginateAndExcludeOtherOrganizations()
    {
        var otherUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(otherUserId, TestOrganizationId);
        var meetingA = await SeedMeetingAsync(TestOrganizationId, "Action meeting A");
        var meetingB = await SeedMeetingAsync(TestOrganizationId, "Action meeting B");
        await SeedMeetingParticipantAsync(meetingA.Id, TestOrganizationId, TestUserId, MeetingRole.Host);
        await SeedMeetingParticipantAsync(meetingA.Id, TestOrganizationId, otherUserId, MeetingRole.Participant);
        await SeedMeetingParticipantAsync(meetingB.Id, TestOrganizationId, TestUserId, MeetingRole.Host);

        var due1 = DateTime.UtcNow.Date.AddDays(2);
        var due2 = due1.AddDays(1);
        var due3 = due1.AddDays(2);

        var minePending = await SeedActionItemAsync(
            TestOrganizationId, meetingA.Id, TestUserId, "Mine pending Trello", due1, ActionItemStatus.PendingReview, ExternalProvider.Trello);
        var otherApproved = await SeedActionItemAsync(
            TestOrganizationId, meetingA.Id, otherUserId, "Other approved ClickUp", due2, ActionItemStatus.Approved, ExternalProvider.ClickUp);
        var mineRejected = await SeedActionItemAsync(
            TestOrganizationId, meetingB.Id, TestUserId, "Mine rejected", due3, ActionItemStatus.Rejected);

        var foreignOrgId = Guid.NewGuid();
        var foreignUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(foreignUserId, foreignOrgId);
        var foreignMeeting = await SeedMeetingAsync(foreignOrgId, "Foreign meeting");
        await SeedMeetingParticipantAsync(foreignMeeting.Id, foreignOrgId, foreignUserId, MeetingRole.Host);
        await SeedActionItemAsync(foreignOrgId, foreignMeeting.Id, foreignUserId, "Foreign item", due1.AddDays(-1));

        var firstPage = await GetOrgActionItemsAsync("page=1&pageSize=2&assignee=all");
        firstPage.TotalCount.Should().Be(3);
        firstPage.Page.Should().Be(1);
        firstPage.PageSize.Should().Be(2);
        firstPage.Items.Should().HaveCount(2);
        firstPage.Items.Select(i => i.Id).Should().Equal(minePending.Id, otherApproved.Id);
        firstPage.Items.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.RowVersionEtag));

        var mineOnly = await GetOrgActionItemsAsync("assignee=me&page=1&pageSize=20");
        mineOnly.TotalCount.Should().Be(2);
        mineOnly.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { minePending.Id, mineRejected.Id });
        mineOnly.Items.Should().OnlyContain(i => i.AssignedToUserId == TestUserId);

        var meetingAndStatus = await GetOrgActionItemsAsync($"meetingId={meetingB.Id}&status=Rejected");
        meetingAndStatus.Items.Should().ContainSingle(i => i.Id == mineRejected.Id);

        var providerFiltered = await GetOrgActionItemsAsync("provider=ClickUp");
        providerFiltered.Items.Should().ContainSingle(i => i.Id == otherApproved.Id);
        providerFiltered.Items[0].ExternalProvider.Should().Be(ExternalProvider.ClickUp.ToString());

        var dateFiltered = await GetOrgActionItemsAsync($"fromUtc={Uri.EscapeDataString(due2.AddHours(-1).ToString("O"))}&toUtc={Uri.EscapeDataString(due2.AddHours(1).ToString("O"))}");
        dateFiltered.Items.Should().ContainSingle(i => i.Id == otherApproved.Id);
    }

    [Fact]
    public async Task ListOrganizationActionItems_CallerTokenForDifferentOrg_ShouldReturnForbidden()
    {
        var otherOrgId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        await SeedUserAndMembershipAsync(otherUserId, otherOrgId);
        var otherClient = CreateAuthenticatedClient(otherUserId, otherOrgId);

        var response = await otherClient.GetAsync($"/api/organizations/{TestOrganizationId}/action-items");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<ActionItemListResponse> GetOrgActionItemsAsync(string query)
    {
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/action-items?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ActionItemListResponse>())!;
    }

    private async Task<string> GetCurrentEtagAsync(Guid meetingId, Guid itemId)
    {
        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/action-items");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<ActionItemListResponse>();
        return list!.Items.Single(i => i.Id == itemId).RowVersionEtag!;
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId, OrganizationRole orgRole = OrganizationRole.Admin)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId, orgRole.ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ApplicationUser> SeedUserAndMembershipAsync(
        Guid userId,
        Guid orgId,
        OrganizationRole orgRole = OrganizationRole.Member,
        bool isEnabled = true)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId))
        {
            db.Organizations.Add(new Organization
            {
                Id = orgId,
                Name = $"Org {orgId:N}",
                Slug = $"org-{orgId:N}"[..20]
            });
        }

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            user = new ApplicationUser
            {
                Id = userId,
                UserName = $"user-{userId:N}@test.com",
                NormalizedUserName = $"USER-{userId:N}@TEST.COM",
                Email = $"user-{userId:N}@test.com",
                NormalizedEmail = $"USER-{userId:N}@TEST.COM",
                EmailConfirmed = true,
                DisplayName = $"User {userId:N}"[..20],
                SecurityStamp = Guid.NewGuid().ToString()
            };
            db.Users.Add(user);
        }

        if (!await db.UserOrgMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == userId && m.OrganizationId == orgId))
        {
            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = userId,
                OrganizationId = orgId,
                OrgRole = orgRole,
                IsEnabled = isEnabled
            });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Meeting> SeedMeetingAsync(Guid orgId, string title = "Action item meeting")
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == orgId))
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = $"Org {orgId:N}", Slug = $"org-{orgId:N}"[..20] });
        }

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Title = title,
            ScheduledStartUtc = DateTime.UtcNow.AddHours(1),
            ScheduledEndUtc = DateTime.UtcNow.AddHours(2),
            Status = MeetingStatus.Scheduled
        };

        db.Meetings.Add(meeting);
        await db.SaveChangesAsync();
        return meeting;
    }

    private async Task<MeetingParticipant> SeedMeetingParticipantAsync(Guid meetingId, Guid orgId, Guid userId, MeetingRole role)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
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
        return participant;
    }

    private async Task<ActionItem> SeedActionItemAsync(
        Guid orgId,
        Guid meetingId,
        Guid? assignedToUserId,
        string title,
        DateTime? dueDateUtc = null,
        ActionItemStatus status = ActionItemStatus.PendingReview,
        ExternalProvider? provider = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var item = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            MeetingId = meetingId,
            Title = title,
            Description = $"Description for {title}",
            AssignedToUserId = assignedToUserId,
            DueDateUtc = dueDateUtc,
            Status = status,
            ExternalProvider = provider,
            ExtractedAtUtc = DateTime.UtcNow
        };

        db.ActionItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }
}
