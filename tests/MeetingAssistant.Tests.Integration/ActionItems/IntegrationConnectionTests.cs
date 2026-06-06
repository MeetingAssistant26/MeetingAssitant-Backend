using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems;

public class IntegrationConnectionTests : IntegrationTestBase<TestWebApplicationFactory>
{
    public IntegrationConnectionTests(TestWebApplicationFactory factory) : base(factory)
    {
    }

    public override async Task InitializeAsync()
    {
        Factory.ResetProviderState();
        await base.InitializeAsync();
    }

    [Fact]
    public async Task SaveProviderCredentials_ShouldRejectEmptyCredentialFields()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/credentials",
            new { apiKey = string.Empty, apiToken = string.Empty });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SaveIntegrationDestination_ShouldRejectEmptyDestinationFields()
    {
        (await SaveCredentialsAsync()).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/destination",
            new { workspaceId = string.Empty, boardId = string.Empty, listId = string.Empty });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SaveProviderCredentials_ShouldEncryptCredentialsAndEnableDiscoveryOnly()
    {
        var response = await SaveCredentialsAsync(apiKey: "qa-api-key", apiToken: "qa-api-token");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storedConfig = await db.OrganizationIntegrationConfigs.SingleAsync(c =>
            c.OrganizationId == TestOrganizationId && c.Provider == ExternalProvider.Trello);
        storedConfig.EncryptedProviderPayload.Should().NotContain("qa-api-key");
        storedConfig.EncryptedProviderPayload.Should().NotContain("qa-api-token");
        storedConfig.SelectedProjectId.Should().BeEmpty();
        storedConfig.SelectedListId.Should().BeEmpty();

        var storedIntegration = await db.OrganizationIntegrations.SingleAsync(i =>
            i.OrganizationId == TestOrganizationId && i.Type == ExternalProvider.Trello);
        storedIntegration.Status.Should().Be(IntegrationStatus.Disabled);

        var config = await GetIntegrationConfigAsync();
        config.HasCredentials.Should().BeTrue();
        config.IsConfigured.Should().BeFalse();
        config.IntegrationStatus.Should().Be(IntegrationStatus.Disabled.ToString());
        config.ProjectId.Should().BeNullOrEmpty();
        config.ListId.Should().BeNullOrEmpty();

        var workspaces = await Client.GetFromJsonAsync<List<ProviderWorkspaceResponse>>(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/workspaces");
        workspaces.Should().ContainSingle(w =>
            w.Id == FakeTaskProviderState.WorkspaceId && w.DisplayName == "QA Workspace");
    }

    [Fact]
    public async Task DestinationDiscoveryAndSave_ShouldExposeOpenBoardsAndActivateSelectedBoardList()
    {
        (await SaveCredentialsAsync()).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var boards = await Client.GetFromJsonAsync<List<ProviderBoardResponse>>(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/workspaces/{FakeTaskProviderState.WorkspaceId}/boards");
        boards.Should().ContainSingle(b => b.Id == FakeTaskProviderState.BoardId);
        boards.Should().NotContain(b => b.Id == FakeTaskProviderState.ClosedBoardId);

        var members = await Client.GetFromJsonAsync<List<ProviderMemberResponse>>(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/boards/{FakeTaskProviderState.BoardId}/members");
        members.Should().Contain(m => m.Id == FakeTaskProviderState.MappedMemberId);

        var invalidDestination = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/destination",
            new
            {
                workspaceId = FakeTaskProviderState.WorkspaceId,
                boardId = FakeTaskProviderState.ClosedBoardId,
                listId = FakeTaskProviderState.ListId
            });
        invalidDestination.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var validDestination = await SaveDestinationAsync();
        validDestination.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var config = await GetIntegrationConfigAsync();
        config.HasCredentials.Should().BeTrue();
        config.IsConfigured.Should().BeTrue();
        config.IntegrationStatus.Should().Be(IntegrationStatus.Active.ToString());
        config.WorkspaceId.Should().Be(FakeTaskProviderState.WorkspaceId);
        config.WorkspaceName.Should().Be("QA Workspace");
        config.ProjectId.Should().Be(FakeTaskProviderState.BoardId);
        config.ProjectName.Should().Be("QA Action Board");
        config.ListId.Should().Be(FakeTaskProviderState.ListId);
        config.ListName.Should().Be("Action Items");
    }

    [Fact]
    public async Task SetMemberMapping_ShouldValidateBoardMemberAndClearMappingWhenEmpty()
    {
        (await SaveCredentialsAsync()).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SaveDestinationAsync()).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var invalidMapping = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/members/{TestUserId}/mapping",
            new { externalMemberId = "not-on-board" });
        invalidMapping.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var validMapping = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/members/{TestUserId}/mapping",
            new { externalMemberId = FakeTaskProviderState.MappedMemberId });
        validMapping.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var mapping = await db.ExternalMemberMappings.SingleAsync(m =>
                m.OrganizationId == TestOrganizationId
                && m.Provider == ExternalProvider.Trello
                && m.UserId == TestUserId);
            mapping.ExternalMemberId.Should().Be(FakeTaskProviderState.MappedMemberId);
        }

        var clearMapping = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/members/{TestUserId}/mapping",
            new { externalMemberId = string.Empty });
        clearMapping.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var exists = await db.ExternalMemberMappings.AnyAsync(m =>
                m.OrganizationId == TestOrganizationId
                && m.Provider == ExternalProvider.Trello
                && m.UserId == TestUserId);
            exists.Should().BeFalse();
        }
    }

    private Task<HttpResponseMessage> SaveCredentialsAsync(
        string apiKey = "qa-api-key",
        string apiToken = "qa-api-token")
        => Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/credentials",
            new { apiKey, apiToken });

    private Task<HttpResponseMessage> SaveDestinationAsync()
        => Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello/destination",
            new
            {
                workspaceId = FakeTaskProviderState.WorkspaceId,
                boardId = FakeTaskProviderState.BoardId,
                listId = FakeTaskProviderState.ListId
            });

    private async Task<IntegrationConfigResponse> GetIntegrationConfigAsync()
    {
        var response = await Client.GetAsync(
            $"/api/organizations/{TestOrganizationId}/integrations/Trello");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<IntegrationConfigResponse>())!;
    }
}
