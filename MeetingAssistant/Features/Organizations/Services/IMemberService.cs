using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Services
{
    public interface IMemberService
    {
        Task<Result<IEnumerable<MemberResponse>>> ListMembersAsync(
            Guid organizationId,
            CancellationToken cancellationToken = default);

        Task<Result> UpdateMemberRoleAsync(
            Guid organizationId,
            Guid userId,
            UpdateMemberRoleRequest request,
            CancellationToken cancellationToken = default);

        Task<Result> UpdateMemberContextAsync(
            Guid organizationId,
            Guid memberUserId,
            UpdateMemberContextRequest request,
            Guid currentUserId,
            CancellationToken cancellationToken = default);
    }
}