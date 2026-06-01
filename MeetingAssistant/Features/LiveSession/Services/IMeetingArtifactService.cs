using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IMeetingArtifactService
    {
        Task<Result<MeetingTranscriptResponse>> GetTranscriptAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);

        Task<Result<MeetingSummaryResponse>> GetSummaryAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken = default);
    }
}
