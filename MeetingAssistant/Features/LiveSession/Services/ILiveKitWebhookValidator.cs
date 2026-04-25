using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface ILiveKitWebhookValidator
    {
        Result<WebhookEvent> Validate(string rawBody, string? authorizationHeader);
    }
}
