using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Features.ActionItems.Models.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Services
{
    public interface IUserIntegrationService
    {
        Task<Result<List<UserIntegrationResponse>>> GetMyConnectionsAsync(
            Guid userId, Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result> ConnectAsync(
            ConnectProviderRequest request,
            Guid userId, Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);

        Task<Result> DisconnectAsync(
            Guid userId, Guid organizationId, ExternalProvider provider,
            CancellationToken cancellationToken = default);
    }
}
