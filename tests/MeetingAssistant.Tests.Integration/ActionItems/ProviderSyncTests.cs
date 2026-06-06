using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems;

public class ProviderSyncTests : IntegrationTestBase<TestWebApplicationFactory>
{
    public ProviderSyncTests(TestWebApplicationFactory factory) : base(factory)
    {
    }

    public override async Task InitializeAsync()
    {
        Factory.ResetProviderState();
        await base.InitializeAsync();
    }

    [Fact]
    public async Task SyncActionItem_ShouldUseExplicitMemberMappingBeforeConnectedAccount()
    {
        await ConfigureIntegrationAsync();
        var (meetingId, actionItemId) = await SeedApprovedActionItemWithConnectedAccountAsync();
        await SaveMappingAsync(FakeTaskProviderState.MappedMemberId);

        var response = await SyncActionItemAsync(meetingId, actionItemId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SyncResultResponse>();
        body!.Status.Should().Be(ActionItemStatus.Synced.ToString());
        body.SyncMissingAssigneeReason.Should().BeNull();
        body.ExternalTaskId.Should().NotBeNullOrWhiteSpace();
        Factory.ProviderState.CreatedTasks.Should().ContainSingle();
        Factory.ProviderState.CreatedTasks.Single().AssigneeExternalId.Should()
            .Be(FakeTaskProviderState.MappedMemberId);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = await db.ActionItems.SingleAsync(i => i.Id == actionItemId);
        item.ExternalProvider.Should().Be(ExternalProvider.Trello);
        item.Status.Should().Be(ActionItemStatus.Synced);
    }

    [Fact]
    public async Task SyncActionItem_WhenMappedMemberLeavesBoard_ShouldSyncWithoutAssigneeAndRecordReason()
    {
        await ConfigureIntegrationAsync();
        var (meetingId, actionItemId) = await SeedApprovedActionItemWithConnectedAccountAsync();
        await SaveMappingAsync(FakeTaskProviderState.MappedMemberId);
        Factory.ProviderState.BoardMembersByBoard[FakeTaskProviderState.BoardId].Clear();

        var response = await SyncActionItemAsync(meetingId, actionItemId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SyncResultResponse>();
        body!.Status.Should().Be(ActionItemStatus.SyncedNoAssignee.ToString());
        body.SyncMissingAssigneeReason.Should().Be("NotProjectMember");
        Factory.ProviderState.CreatedTasks.Should().ContainSingle();
        Factory.ProviderState.CreatedTasks.Single().AssigneeExternalId.Should().BeNull();
    }

    [Fact]
    public async Task SyncActionItem_WithConnectedAccountButNoExplicitMapping_ShouldSyncWithoutAssignee()
    {
        await ConfigureIntegrationAsync();
        var (meetingId, actionItemId) = await SeedApprovedActionItemWithConnectedAccountAsync();

        var response = await SyncActionItemAsync(meetingId, actionItemId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SyncResultResponse>();
        body!.Status.Should().Be(ActionItemStatus.SyncedNoAssignee.ToString());
        body.SyncMissingAssigneeReason.Should().Be("UserNotConnected");
        Factory.ProviderState.CreatedTasks.Should().ContainSingle();
        Factory.ProviderState.CreatedTasks.Single().AssigneeExternalId.Should().BeNull();
    }

    private async Task ConfigureIntegrationAsync()
    {
        var credentials = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/credentials",
            new { apiKey = "qa-api-key", apiToken = "qa-api-token" });
        credentials.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var destination = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/destination",
            new
            {
                workspaceId = FakeTaskProviderState.WorkspaceId,
                boardId = FakeTaskProviderState.BoardId,
                listId = FakeTaskProviderState.ListId
            });
        destination.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task SaveMappingAsync(string externalMemberId)
    {
        var mapping = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/members/{TestUserId}/mapping",
            new { externalMemberId });
        mapping.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<(Guid MeetingId, Guid ActionItemId)> SeedApprovedActionItemWithConnectedAccountAsync()
    {
        var meetingId = Guid.NewGuid();
        var actionItemId = Guid.NewGuid();

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = TestOrganizationId,
            Title = "Trello provider sync test",
            ScheduledStartUtc = DateTime.UtcNow.AddHours(1),
            ScheduledEndUtc = DateTime.UtcNow.AddHours(2),
            Status = MeetingStatus.Scheduled
        });
        db.MeetingParticipants.Add(new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            OrganizationId = TestOrganizationId,
            UserId = TestUserId,
            MeetingRole = MeetingRole.Host
        });
        db.ExternalAccountLinks.Add(new ExternalAccountLink
        {
            OrganizationId = TestOrganizationId,
            UserId = TestUserId,
            Provider = ExternalProvider.Trello,
            ExternalUserId = FakeTaskProviderState.ConnectedMemberId,
            ExternalUsername = "connected.trello",
            AccessTokenProtected = "protected-token"
        });
        db.ActionItems.Add(new ActionItem
        {
            Id = actionItemId,
            OrganizationId = TestOrganizationId,
            MeetingId = meetingId,
            Title = "Prepare Trello validation checklist",
            Description = "QA Bob prepares the Trello validation checklist.",
            AssignedToUserId = TestUserId,
            DueDateUtc = DateTime.UtcNow.AddDays(3),
            Status = ActionItemStatus.Approved,
            ExtractedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return (meetingId, actionItemId);
    }

    private async Task<HttpResponseMessage> SyncActionItemAsync(Guid meetingId, Guid actionItemId)
    {
        var list = await Client.GetFromJsonAsync<ActionItemListResponse>(
            $"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/action-items");
        var etag = list!.Items.Single(item => item.Id == actionItemId).RowVersionEtag;

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/action-items/{actionItemId}/sync");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await Client.SendAsync(request);
    }
}
