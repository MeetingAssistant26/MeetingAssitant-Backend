using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.AgentApi.Services
{
    public interface IAgentContextService
    {
        Task<Result<AgentOrganizationResponse>> GetOrganizationAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result<IReadOnlyList<AgentMemberResponse>>> GetMeetingMembersAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);

        Task<Result<AgentMeetingListResponse>> ListMeetingsAsync(
            Guid organizationId,
            string? status,
            int limit,
            int offset,
            CancellationToken cancellationToken = default);

        Task<Result<AgentMeetingDetailResponse>> GetMeetingDetailAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);

        Task<Result<IReadOnlyList<AgentMeetingResponse>>> ListRecurringMeetingsAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result<IReadOnlyList<MeetingTagResponse>>> ListMeetingTagsAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default);
    }
}
