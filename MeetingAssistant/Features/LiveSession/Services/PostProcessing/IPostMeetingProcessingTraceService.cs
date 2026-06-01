using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services.PostProcessing
{
    public interface IPostMeetingProcessingTraceService
    {
        Task<Result<PostMeetingProcessingTraceResponse>> GetTracesAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);
    }
}
