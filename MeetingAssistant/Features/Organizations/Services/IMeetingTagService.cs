using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.Organizations.Services
{
    public interface IMeetingTagService
    {
        Task<Result<MeetingTagResponse>> CreateAsync(CreateMeetingTagRequest request, CancellationToken ct);
        Task<Result<MeetingTagResponse>> UpdateAsync(Guid tagId, UpdateMeetingTagRequest request, CancellationToken ct);
        Task<Result> DeleteAsync(Guid tagId, CancellationToken ct);
        Task<Result<IEnumerable<MeetingTagResponse>>> ListAsync(CancellationToken ct);
    }
}
