using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IWebhookService
    {
        Task<Result> ProcessAsync(
            WebhookEvent webhookEvent,
            string rawPayload,
            CancellationToken cancellationToken = default);
    }
}
