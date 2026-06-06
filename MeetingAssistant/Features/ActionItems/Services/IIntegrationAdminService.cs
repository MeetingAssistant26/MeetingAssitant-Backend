using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Services
{
    public interface IIntegrationAdminService
    {
        Task<Result<IntegrationConfigResponse>> GetConfigAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result> SaveConfigAsync(
            SaveIntegrationConfigRequest request,
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result> SaveCredentialsAsync(
            SaveProviderCredentialsRequest request,
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result> SaveDestinationAsync(
            SaveIntegrationDestinationRequest request,
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result<List<ProviderWorkspaceResponse>>> GetWorkspacesAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result<List<ProviderBoardResponse>>> GetBoardsAsync(
            Guid organizationId, ExternalProvider provider, string workspaceId,
            CancellationToken cancellationToken = default);

        Task<Result<List<ProviderMemberResponse>>> GetBoardMembersAsync(
            Guid organizationId, ExternalProvider provider, string boardId,
            CancellationToken cancellationToken = default);

        Task<Result<List<ProviderProjectResponse>>> GetProjectsAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result<List<ProviderListResponse>>> GetListsAsync(
            Guid organizationId, ExternalProvider provider, string projectId,
            CancellationToken cancellationToken = default);

        Task<Result<List<MemberProviderStatusResponse>>> GetMemberStatusAsync(
            Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result> SetMemberMappingAsync(
            SetMemberMappingRequest request,
            Guid organizationId, ExternalProvider provider, Guid userId,
            CancellationToken cancellationToken = default);
    }
}
