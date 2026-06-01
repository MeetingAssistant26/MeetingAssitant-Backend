using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IAiDebugTraceService
    {
        Task<Result> IngestAsync(
            Guid organizationId,
            Guid meetingId,
            AiDebugTraceIngestRequest request,
            CancellationToken cancellationToken = default);

        Task<Result<AiDebugTraceResponse>> GetTracesAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default);
    }
}
