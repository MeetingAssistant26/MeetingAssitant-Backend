using MeetingAssistant.Features.ActionItems.Models.Entities;

namespace MeetingAssistant.Features.ActionItems.Services.Abstractions
{
    public interface ITaskProvider
    {
        string ProviderName { get; }

        Task<ProviderHealthResult> ValidateCredentialsAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ProviderWorkspace>> ListWorkspacesAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ProviderBoard>> ListBoardsAsync(
            OrganizationIntegrationConfig config,
            string workspaceId,
            bool openOnly,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ProviderMember>> ListBoardMembersAsync(
            OrganizationIntegrationConfig config,
            string boardId,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ProviderList>> ListListsAsync(
            OrganizationIntegrationConfig config,
            string projectId,
            CancellationToken cancellationToken = default);

        Task<ProviderTaskResult> CreateTaskAsync(
            OrganizationIntegrationConfig config,
            ProviderTaskRequest request,
            CancellationToken cancellationToken = default);

        Task<bool> ValidateAssigneeAsync(
            OrganizationIntegrationConfig config,
            string projectId,
            string assigneeExternalId,
            CancellationToken cancellationToken = default);
    }
}
