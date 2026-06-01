using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Services
{
    public interface IOrganizationService
    {
        Task<Result<OrganizationResponse>> CreateOrganizationAsync(
            CreateOrganizationRequest request,
            Guid userId,
            CancellationToken cancellationToken = default);

        Task<Result<OrganizationListResponse>> ListOrganizationsAsync(
            Guid userId,
            CancellationToken cancellationToken = default);

        Task<Result<OrganizationResponse>> GetOrganizationAsync(
            Guid organizationId,
            Guid userId,
            CancellationToken cancellationToken = default);

        Task<Result> LeaveOrganizationAsync(
            Guid organizationId,
            Guid userId,
            CancellationToken cancellationToken = default);
    }
}
