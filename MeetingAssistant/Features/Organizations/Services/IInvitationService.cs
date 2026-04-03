using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Services
{
    public interface IInvitationService
    {
        Task<Result<CreateInvitationResponse>> CreateInvitationAsync(
            Guid organizationId,
            Guid userId,
            CreateInvitationRequest request,
            CancellationToken cancellationToken = default);

        Task<Result> RevokeInvitationAsync(
            Guid organizationId,
            Guid userId,
            Guid invitationId,
            CancellationToken cancellationToken = default);
    }
}