using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.AgentApi.Services
{
    public interface IAgentAuthService
    {
        Task<Result<string>> MintTokenAsync(
            Guid organizationId,
            Guid meetingId,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default);

        Task<Result<string>> RefreshTokenAsync(
            string currentToken,
            CancellationToken cancellationToken = default);
    }
}
