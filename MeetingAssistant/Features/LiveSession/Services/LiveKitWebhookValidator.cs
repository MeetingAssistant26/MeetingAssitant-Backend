using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class LiveKitWebhookValidator(
        IOptions<LiveKitOptions> options,
        ILogger<LiveKitWebhookValidator> logger) : ILiveKitWebhookValidator
    {
        private readonly LiveKitOptions _options = options.Value;
        private readonly ILogger<LiveKitWebhookValidator> _logger = logger;

        public Result<WebhookEvent> Validate(string rawBody, string? authorizationHeader)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                return Result.Failure<WebhookEvent>(LiveSessionErrors.InvalidWebhookSignature);
            }

            var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret)
                ? _options.ApiSecret
                : _options.WebhookSecret;

            if (string.IsNullOrWhiteSpace(secret))
            {
                return Result.Failure<WebhookEvent>(LiveSessionErrors.InvalidWebhookSignature);
            }

            try
            {
                var receiver = new WebhookReceiver(_options.ApiKey, secret);
                var eventPayload = receiver.Receive(rawBody, authorizationHeader ?? string.Empty, true, true);
                return Result.Success(eventPayload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LiveKit webhook signature validation failed.");
                return Result.Failure<WebhookEvent>(LiveSessionErrors.InvalidWebhookSignature);
            }
        }
    }
}
